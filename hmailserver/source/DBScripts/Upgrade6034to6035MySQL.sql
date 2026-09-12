create table hm_files
(
	fileid int auto_increment not null, primary key(`fileid`), unique(`fileid`),
	fileaccountid int not null,
	filetoken varchar(64) not null,
	filename varchar(255) not null,
	filetype varchar(100) not null,
	filesize bigint not null,
	filestored bigint not null,
	filecomplete tinyint not null,
	filecreated bigint not null,
	fileexpires bigint not null,
	filepasswordhash varchar(64) not null,
	filedownloads int not null
) DEFAULT CHARSET=utf8;

CREATE INDEX idx_hm_files_account ON hm_files (fileaccountid);

CREATE UNIQUE INDEX idx_hm_files_token ON hm_files (filetoken);

ALTER TABLE hm_files ENGINE=InnoDB;

ALTER TABLE hm_files ADD CONSTRAINT fk_hm_files_account FOREIGN KEY (fileaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6035;
