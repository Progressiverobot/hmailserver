create table hm_files
(
	fileid bigserial not null primary key,
	fileaccountid int not null,
	filetoken varchar(64) not null,
	filename varchar(255) not null,
	filetype varchar(100) not null,
	filesize bigint not null,
	filestored bigint not null,
	filecomplete smallint not null,
	filecreated bigint not null,
	fileexpires bigint not null,
	filepasswordhash varchar(64) not null,
	filedownloads int not null
);

CREATE INDEX idx_hm_files_account ON hm_files (fileaccountid);
CREATE UNIQUE INDEX idx_hm_files_token ON hm_files (filetoken);

ALTER TABLE hm_files ADD CONSTRAINT fk_hm_files_account FOREIGN KEY (fileaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6035;
