create table hm_contacts
(
	contactid int auto_increment not null, primary key(`contactid`), unique(`contactid`),
	contactaccountid int not null,
	contactname varchar(255) not null,
	contactaddress varchar(255) not null,
	contactsource tinyint not null,
	contactcreated datetime not null
) DEFAULT CHARSET=utf8;

CREATE UNIQUE INDEX idx_hm_contacts_account ON hm_contacts (contactaccountid, contactaddress);

ALTER TABLE hm_contacts ENGINE=InnoDB;

ALTER TABLE hm_contacts ADD CONSTRAINT fk_hm_contacts_account FOREIGN KEY (contactaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

update hm_dbversion set value = 6032;
